using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// Drop down list that paints itself instead of being painted over.
///
/// <para>
/// The control is a native window, and the approach this replaces - letting
/// comctl32 draw and then covering the parts that came out light - is what made the
/// drop downs flicker: every hover, every focus change and every repaint drew the
/// control twice, once in the system colours and once in the palette. Windows Forms
/// has a way out that needs no overlay at all. ComboBox.WndProc handles WM_PAINT
/// itself only while ControlStyles.UserPaint is off; with it on, the message falls
/// through to Control.WndProc, which routes WM_PAINT to WmPaint - a back buffer and
/// a call to OnPaint - instead of to DefWndProc. The native control never draws, so
/// there is nothing left to correct and nothing to flash, and the frame, the value
/// and the chevron all come out of one paint instead of three.
/// </para>
/// <para>
/// Where the parts go is not guessed at either: GetComboBoxInfo answers with the
/// text rectangle and the button rectangle the control would have used itself, so
/// the painting lands exactly where comctl32 would have put it.
/// </para>
/// <para>
/// What stays outside all of this is the list window, which is a window of its own:
/// its background follows BackColor (comctl32 asks for it with WM_CTLCOLORLISTBOX),
/// its entries are drawn by <see cref="OnDrawItem"/>, and its frame and scroll bar
/// are darkened with the DarkMode_Explorer window theme.
/// </para>
/// </summary>
internal sealed class ThemedComboBox : ComboBox
{
    /// <summary>
    /// The border a Win32 combo box keeps around its text area (COMBO_XBORDERSIZE),
    /// used only when GetComboBoxInfo has nothing to say.
    /// </summary>
    private const int FieldBorder = 2;

    private bool _hot;

    public ThemedComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        // Owner drawn entries: the list is a native window that would otherwise
        // paint its text in the system colours.
        DrawMode = DrawMode.OwnerDrawFixed;
        // Never changed afterwards. Swapping FlatStyle with the theme changes how
        // the control measures itself, and the dialog is sized once.
        FlatStyle = FlatStyle.Standard;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
    }

    /// <summary>Applies the current palette, including to the list window.</summary>
    public void ApplyTheme()
    {
        ThemePalette palette = ThemeManager.Palette;
        BackColor = palette.Field;
        ForeColor = palette.Text;
        ApplyListWindowTheme();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyListWindowTheme();
    }

    /// <summary>
    /// The themed face has rounded corners and leaves them uncovered, so what shows
    /// through has to be the dialog behind the control - not BackColor, which is the
    /// field colour the list window reads.
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using SolidBrush brush = new SolidBrush(Parent?.BackColor ?? ThemeManager.Palette.Window);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemePalette palette = ThemeManager.Palette;
        Graphics graphics = e.Graphics;
        Rectangle bounds = ClientRectangle;
        PartState state = ControlPainter.StateOf(Enabled, DroppedDown, _hot);

        GetPartBounds(out Rectangle field, out Rectangle button);
        ControlPainter.DrawComboBox(graphics, bounds, button, state);

        Color text = palette.TextFor(state);

        // A focused drop down list shows its value in the selection colours - that is
        // what Windows does, and with the palette behind it there is no system blue
        // left to leak into the dark theme.
        if (Focused && Enabled)
        {
            using (SolidBrush brush = new SolidBrush(palette.Selection))
            {
                graphics.FillRectangle(brush, field);
            }

            text = palette.SelectionText;

            if (ShowFocusCues)
            {
                ControlPainter.DrawFocusRectangle(graphics, field, palette.SelectionText, palette.Selection);
            }
        }

        TextRenderer.DrawText(graphics, CurrentText(), Font, field, text, ControlPainter.FieldFlags);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        // Index -1 is an empty list; the closed control never reaches this method,
        // because the native control no longer paints and so never asks for it.
        if (e.Index < 0 || e.Index >= Items.Count)
        {
            return;
        }

        ThemePalette palette = ThemeManager.Palette;
        bool selected = (e.State & DrawItemState.Selected) != 0;

        using (SolidBrush background = new SolidBrush(selected ? palette.Selection : palette.Field))
        {
            e.Graphics.FillRectangle(background, e.Bounds);
        }

        // Two pixels of inset, which is where a native list box starts its own text.
        Rectangle text = Rectangle.FromLTRB(
            e.Bounds.X + DpiScale.Round(2), e.Bounds.Y, e.Bounds.Right, e.Bounds.Bottom);
        TextRenderer.DrawText(
            e.Graphics,
            GetItemText(Items[e.Index]),
            Font,
            text,
            selected ? palette.SelectionText : palette.Text,
            ControlPainter.FieldFlags);

        e.DrawFocusRectangle();
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        Invalidate();
        base.OnSelectedIndexChanged(e);
    }

    protected override void OnDropDown(EventArgs e)
    {
        Invalidate();
        base.OnDropDown(e);
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        Invalidate();
        base.OnDropDownClosed(e);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hot = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = false;
        Invalidate();
        base.OnMouseLeave(e);
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

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    private string CurrentText()
        => SelectedIndex >= 0 && SelectedIndex < Items.Count ? GetItemText(Items[SelectedIndex]) : string.Empty;

    /// <summary>
    /// Where the value and the chevron go. The control is asked first; the fallback
    /// is the geometry a Win32 combo box uses - a button as wide as a vertical
    /// scroll bar, inside the two pixel border, and the text in what is left.
    /// </summary>
    private void GetPartBounds(out Rectangle field, out Rectangle button)
    {
        if (TryGetComboBoxInfo(out NativeMethods.COMBOBOXINFO info)
            && info.rcButton.Right > info.rcButton.Left
            && info.rcItem.Right > info.rcItem.Left)
        {
            field = info.rcItem.ToRectangle();
            button = info.rcButton.ToRectangle();
            return;
        }

        Rectangle bounds = ClientRectangle;
        int width = Math.Min(
            SystemInformation.VerticalScrollBarWidth,
            Math.Max(0, bounds.Width - (FieldBorder * 2)));
        button = new Rectangle(
            bounds.Right - FieldBorder - width,
            bounds.Y + FieldBorder,
            width,
            Math.Max(0, bounds.Height - (FieldBorder * 2)));
        field = Rectangle.FromLTRB(
            bounds.X + FieldBorder + 1,
            bounds.Y + FieldBorder,
            Math.Max(bounds.X + FieldBorder + 1, button.Left),
            bounds.Bottom - FieldBorder);
    }

    private bool TryGetComboBoxInfo(out NativeMethods.COMBOBOXINFO info)
    {
        info = default;
        if (!IsHandleCreated)
        {
            return false;
        }

        info.cbSize = Marshal.SizeOf(typeof(NativeMethods.COMBOBOXINFO));
        try
        {
            return NativeMethods.GetComboBoxInfo(Handle, ref info);
        }
        catch (Exception ex)
        {
            Logger.Debug("GetComboBoxInfo failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Darkens the list window: its frame, and the scroll bar it grows when there
    /// are more entries than fit. Both belong to a window this control owns but does
    /// not paint, so a theme is the only way to reach them.
    /// </summary>
    private void ApplyListWindowTheme()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        bool dark = ThemeManager.Palette.IsDark;
        DarkModeNative.AllowDarkModeForHandle(Handle, dark);

        if (!TryGetComboBoxInfo(out NativeMethods.COMBOBOXINFO info) || info.hwndList == IntPtr.Zero)
        {
            return;
        }

        DarkModeNative.AllowDarkModeForHandle(info.hwndList, dark);
        DarkModeNative.ApplyWindowTheme(info.hwndList, dark ? "DarkMode_Explorer" : null);
    }
}
