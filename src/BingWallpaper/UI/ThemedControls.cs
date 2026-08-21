using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// Shared plumbing of the owner drawn controls: the hover and pressed flags every
/// themed part needs, and a repaint whenever anything the paint depends on changes.
/// </summary>
internal abstract class ThemedControlBase : Control
{
    private bool _hot;
    private bool _pressed;

    protected ThemedControlBase()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
    }

    protected PartState PaintState => ControlPainter.StateOf(Enabled, _pressed, _hot);

    /// <summary>
    /// A measuring surface. Windows Forms measures against the screen for the same
    /// reason: the control may not have a window yet, and the size of a themed part
    /// only depends on the device it will be drawn to.
    /// </summary>
    protected static Graphics CreateMeasurementGraphics() => Graphics.FromHwnd(IntPtr.Zero);

    protected override void OnMouseEnter(EventArgs e)
    {
        _hot = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = false;
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

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_pressed)
        {
            _pressed = false;
            Invalidate();
        }

        base.OnMouseUp(e);
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
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Invalidate();
        base.OnTextChanged(e);
    }
}

/// <summary>
/// A check box or a radio button: the same control apart from the glyph, which is
/// why Windows Forms builds both of them from one layout as well.
///
/// <para>
/// Neither derives from CheckBox or RadioButton. Those two paint their glyph
/// through the system theme against their own background, which is a black mark on
/// a black circle once that background is dark, and every way out of it -
/// FlatStyle, Appearance, UserPaint - changes how the control measures itself, so
/// the dialog would be laid out differently in the two themes. Painting both states
/// here keeps one geometry, taken from the theme, for both of them.
/// </para>
/// </summary>
internal abstract class ThemedGlyphControl : ThemedControlBase
{
    private bool _checked;

    protected ThemedGlyphControl(string text)
    {
        Text = text;
        AutoSize = true;
    }

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }

            _checked = value;
            Invalidate();
            OnCheckedChanged(EventArgs.Empty);
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        using Graphics graphics = CreateMeasurementGraphics();
        int glyph = ControlPainter.GlyphSize(graphics);
        if (Text.Length == 0)
        {
            // Captionless: a row label carries the text, so the glyph is all there is.
            return ControlPainter.MeasureGlyphOnly(glyph);
        }

        Size caption = TextRenderer.MeasureText(
            graphics,
            Text,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            ControlPainter.CaptionFlags);
        return ControlPainter.MeasureGlyphControl(caption, glyph);
    }

    protected abstract void DrawGlyph(Graphics graphics, Rectangle glyph, bool isChecked, PartState state);

    /// <summary>What a click does: a check box toggles, a radio button only sets.</summary>
    protected abstract void Toggle();

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemePalette palette = ThemeManager.Palette;
        Graphics graphics = e.Graphics;
        graphics.Clear(BackColor);

        PartState state = PaintState;
        int size = ControlPainter.GlyphSize(graphics);
        bool captionless = Text.Length == 0;
        Rectangle glyph = captionless
            ? ControlPainter.CentredGlyphBounds(ClientRectangle, size)
            : ControlPainter.GlyphBounds(ClientRectangle, size);
        DrawGlyph(graphics, glyph, Checked, state);

        if (captionless)
        {
            // There is no text to ring, so the focus shows around the glyph instead -
            // without it the control would be reachable by keyboard and give no sign
            // of holding the focus.
            if (Focused && ShowFocusCues)
            {
                ControlPainter.DrawFocusRectangle(graphics, ClientRectangle, ForeColor, BackColor);
            }

            return;
        }

        Rectangle caption = ControlPainter.CaptionBounds(ClientRectangle, size);
        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            caption,
            state == PartState.Disabled ? palette.DisabledText : ForeColor,
            ControlPainter.CaptionFlags);

        // ShowFocusCues, not Focused alone: Windows keeps focus rectangles hidden
        // until the keyboard has been used, and the control that happens to hold the
        // focus when a window opens should not be ringed.
        if (Focused && ShowFocusCues)
        {
            Size text = TextRenderer.MeasureText(
                graphics, Text, Font, new Size(int.MaxValue, int.MaxValue), ControlPainter.CaptionFlags);
            Rectangle focus = new Rectangle(
                caption.X,
                caption.Y + ((caption.Height - text.Height) / 2),
                Math.Min(text.Width, caption.Width),
                text.Height);
            ControlPainter.DrawFocusRectangle(graphics, focus, ForeColor, BackColor);
        }
    }

    protected virtual void OnCheckedChanged(EventArgs e) => CheckedChanged?.Invoke(this, e);

    protected override void OnClick(EventArgs e)
    {
        Toggle();
        base.OnClick(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData)
        => keyData == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            e.Handled = true;
            Toggle();
            return;
        }

        base.OnKeyDown(e);
    }
}

/// <summary>Check box with the metrics of a stock one.</summary>
internal sealed class ThemedCheckBox : ThemedGlyphControl
{
    public ThemedCheckBox(string text)
        : base(text)
    {
    }

    protected override void DrawGlyph(Graphics graphics, Rectangle glyph, bool isChecked, PartState state)
        => ControlPainter.DrawCheckBoxGlyph(graphics, glyph, isChecked, state);

    protected override void Toggle() => Checked = !Checked;
}

/// <summary>
/// Radio button with the metrics of a stock one. Like the stock control it takes
/// its group from its parent, so a dialog with two groups needs two containers.
/// </summary>
internal sealed class ThemedRadioButton : ThemedGlyphControl
{
    public ThemedRadioButton(string text)
        : base(text)
    {
    }

    protected override void DrawGlyph(Graphics graphics, Rectangle glyph, bool isChecked, PartState state)
        => ControlPainter.DrawRadioGlyph(graphics, glyph, isChecked, state);

    /// <summary>Clicking a radio button selects it; clicking it again does nothing.</summary>
    protected override void Toggle() => Checked = true;

    protected override void OnCheckedChanged(EventArgs e)
    {
        if (Checked)
        {
            ClearSiblings();
        }

        UpdateGroupTabStops();
        base.OnCheckedChanged(e);
    }

    /// <summary>The arrow keys move inside the group, as they do in a dialog.</summary>
    protected override bool IsInputKey(Keys keyData) => keyData switch
    {
        Keys.Left or Keys.Right or Keys.Up or Keys.Down => true,
        _ => base.IsInputKey(keyData),
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int delta = e.KeyCode switch
        {
            Keys.Left or Keys.Up => -1,
            Keys.Right or Keys.Down => 1,
            _ => 0,
        };

        if (delta != 0 && MoveSelection(delta))
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// The exclusion a stock radio button gets from Win32: only one button per
    /// parent stays checked.
    /// </summary>
    private void ClearSiblings()
    {
        foreach (ThemedRadioButton sibling in Group())
        {
            if (!ReferenceEquals(sibling, this))
            {
                sibling.Checked = false;
            }
        }
    }

    /// <summary>Checks the neighbour <paramref name="delta"/> steps away, wrapping around.</summary>
    private bool MoveSelection(int delta)
    {
        List<ThemedRadioButton> group = Group();
        int index = group.IndexOf(this);
        if (group.Count < 2 || index < 0)
        {
            return false;
        }

        int target = ((index + delta) % group.Count + group.Count) % group.Count;
        group[target].Checked = true;
        group[target].Focus();
        return true;
    }

    /// <summary>
    /// Only one button of a group is a tab stop, which is the WS_TABSTOP a Win32
    /// dialog gives to the checked one: Tab enters the group once and lands on the
    /// current answer, and the arrow keys move from there. The first button keeps it
    /// while nothing is checked, so the group can never become unreachable.
    /// </summary>
    private void UpdateGroupTabStops()
    {
        List<ThemedRadioButton> group = Group();
        ThemedRadioButton? stop = null;
        foreach (ThemedRadioButton button in group)
        {
            if (button.Checked)
            {
                stop = button;
                break;
            }
        }

        stop ??= group.Count > 0 ? group[0] : null;
        foreach (ThemedRadioButton button in group)
        {
            button.TabStop = ReferenceEquals(button, stop);
        }
    }

    private List<ThemedRadioButton> Group()
    {
        List<ThemedRadioButton> group = new List<ThemedRadioButton>();
        if (Parent is null)
        {
            group.Add(this);
            return group;
        }

        foreach (Control sibling in Parent.Controls)
        {
            if (sibling is ThemedRadioButton radio)
            {
                group.Add(radio);
            }
        }

        return group;
    }
}

/// <summary>
/// Push button.
///
/// <para>
/// The caption goes straight into the client rectangle rather than into the box a
/// stock button centres it in: Windows Forms derives that box from the border
/// width, the focus rectangle and the padding, which with the Chinese UI font
/// leaves the caption visibly low, and no property from the outside can correct it
/// - height and padding both move the box the text is centred in.
/// </para>
/// </summary>
internal sealed class ThemedButton : ThemedControlBase, IButtonControl
{
    private bool _isDefault;

    public ThemedButton()
    {
        TabStop = true;
    }

    public DialogResult DialogResult { get; set; }

    public void NotifyDefault(bool value)
    {
        if (_isDefault != value)
        {
            _isDefault = value;
            Invalidate();
        }
    }

    public void PerformClick()
    {
        if (CanSelect)
        {
            OnClick(EventArgs.Empty);
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        using Graphics graphics = CreateMeasurementGraphics();
        Size caption = TextRenderer.MeasureText(
            graphics, Text, Font, new Size(int.MaxValue, int.MaxValue), ControlPainter.CaptionFlags);
        return new Size(caption.Width + DpiScale.Round(16), caption.Height + DpiScale.Round(10));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemePalette palette = ThemeManager.Palette;
        Graphics graphics = e.Graphics;
        PartState state = PaintState;

        ControlPainter.DrawPushButton(graphics, ClientRectangle, state, _isDefault);

        Color caption = state == PartState.Disabled ? palette.DisabledText : ForeColor;
        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            ClientRectangle,
            caption,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        if (Focused && ShowFocusCues)
        {
            int inset = DpiScale.Round(3);
            Rectangle focus = Rectangle.Inflate(ClientRectangle, -inset, -inset);
            ControlPainter.DrawFocusRectangle(graphics, focus, caption, palette.FaceFor(state));
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
    }

    protected override void OnClick(EventArgs e)
    {
        // Before the event, which is the order a stock button uses: a handler is
        // then still free to take the result back and keep the dialog open.
        Form? form = FindForm();
        if (form is not null && DialogResult != DialogResult.None)
        {
            form.DialogResult = DialogResult;
        }

        base.OnClick(e);
    }

    protected override bool IsInputKey(Keys keyData)
        => keyData == Keys.Space || base.IsInputKey(keyData);

    /// <summary>
    /// Space clicks the button, as it does for a stock one. Enter is not handled
    /// here: a focused button is the default button of its form, so the form has
    /// already turned that key into a click before it could arrive.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            e.Handled = true;
            PerformClick();
            return;
        }

        base.OnKeyDown(e);
    }
}

/// <summary>
/// One pixel horizontal rule. A plain Panel would do, except that the theme paints
/// every unrecognised control with the window background, which would hide the line.
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
        using SolidBrush brush = new SolidBrush(ThemeManager.Palette.Border);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }
}
