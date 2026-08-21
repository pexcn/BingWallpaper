using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// The one place that knows what a dialog control part looks like. Every owner
/// drawn control in the settings window asks it for both its metrics and its
/// pixels, so the two can never drift apart.
///
/// <para>
/// It follows a single rule: <b>in the light theme the visual styles draw the
/// part, in the dark theme the palette does</b>. Light is therefore not an
/// imitation of a Windows Forms control - it is the very uxtheme call the stock
/// control makes, through <see cref="CheckBoxRenderer"/>,
/// <see cref="RadioButtonRenderer"/>, <see cref="ButtonRenderer"/> and the
/// COMBOBOX theme class - so a check box here and a check box in any other dialog
/// on the machine are the same pixels. Windows has no themed parts for dark dialog
/// controls (the DarkMode_* theme classes cover list views, scroll bars, menus and
/// edit frames, not check boxes or push buttons), so those are painted from the
/// palette - with the metrics still read from the theme, which keeps the layout
/// identical in both themes and at every DPI.
/// </para>
/// <para>
/// The geometry is the one Windows Forms composes in
/// ButtonInternal.LayoutOptions: the glyph is followed by one pixel of slack and
/// two more of text inset, and the preferred size is the glyph box - one pixel
/// wider than the glyph - plus the caption with that inset on both sides.
/// Reproducing those numbers is what makes the controls line up with the stock
/// ones instead of merely resembling them.
/// </para>
/// </summary>
internal static class ControlPainter
{
    /// <summary>uxtheme class of the drop down parts (vsstyle.h).</summary>
    private const string ComboBoxClass = "COMBOBOX";

    /// <summary>CP_READONLY: the whole face of a drop down <em>list</em>.</summary>
    private const int ComboBoxReadOnlyPart = 5;

    /// <summary>CP_DROPDOWNBUTTONRIGHT: the chevron, drawn transparently over the face.</summary>
    private const int ComboBoxButtonPart = 6;

    /// <summary>
    /// LayoutOptions.standardCheckSize - the glyph side Windows Forms falls back to
    /// when there is no theme to ask. Scaled here, because the themed size arrives
    /// already scaled.
    /// </summary>
    private const int StandardGlyphSize = 13;

    /// <summary>
    /// The two parts a themed drop down list is made of. The state they carry here
    /// is only the one they are looked up with; the state actually drawn is set on
    /// the renderer, and a theme that defines a part defines all four of its states.
    /// </summary>
    private static readonly VisualStyleElement ComboBoxFace =
        VisualStyleElement.CreateElement(ComboBoxClass, ComboBoxReadOnlyPart, (int)PartState.Normal);

    private static readonly VisualStyleElement ComboBoxChevron =
        VisualStyleElement.CreateElement(ComboBoxClass, ComboBoxButtonPart, (int)PartState.Normal);

    private static VisualStyleRenderer? _renderer;

    /// <summary>Text flags shared by every caption, so measuring and drawing agree.</summary>
    public static TextFormatFlags CaptionFlags =>
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    /// <summary>Text flags of a value inside a field, which may be too long for it.</summary>
    public static TextFormatFlags FieldFlags =>
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

    /// <summary>The pixel of slack Windows Forms leaves between glyph and text field.</summary>
    private static int GlyphGap => DpiScale.Round(1);

    /// <summary>LayoutOptions.textImageInset, the inset around a caption.</summary>
    private static int TextInset => DpiScale.Round(2);

    public static PartState StateOf(bool enabled, bool pressed, bool hot)
    {
        if (!enabled)
        {
            return PartState.Disabled;
        }

        return pressed ? PartState.Pressed : hot ? PartState.Hot : PartState.Normal;
    }

    /// <summary>
    /// Side of a check box or radio button glyph. Read from the theme so it follows
    /// the system DPI, and asked for in both themes: the dark glyph is painted by
    /// hand, but it has to occupy exactly the box the light one would.
    /// </summary>
    public static int GlyphSize(Graphics graphics)
    {
        try
        {
            // Without a theme the renderer answers a flat 13, which is only right at
            // 96 DPI; the scaled constant below is the better answer then.
            if (VisualStyleRenderer.IsSupported)
            {
                int size = CheckBoxRenderer.GetGlyphSize(graphics, CheckBoxState.UncheckedNormal).Width;
                if (size > 0)
                {
                    return size;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug("Could not read the themed check box size: " + ex.Message);
        }

        return DpiScale.Round(StandardGlyphSize);
    }

    /// <summary>
    /// Box the glyph occupies inside a check box or radio button: hard against the
    /// left edge, centred on the height, exactly as LayoutOptions places it.
    /// </summary>
    public static Rectangle GlyphBounds(Rectangle client, int glyphSize)
        => new Rectangle(
            client.X,
            client.Y + ((client.Height - glyphSize) / 2),
            glyphSize,
            glyphSize);

    /// <summary>
    /// Box the glyph occupies when the control carries no caption: centred, with
    /// the caption inset left as padding all round, so the focus rectangle has
    /// somewhere to go.
    /// </summary>
    public static Rectangle CentredGlyphBounds(Rectangle client, int glyphSize)
        => new Rectangle(
            client.X + ((client.Width - glyphSize) / 2),
            client.Y + ((client.Height - glyphSize) / 2),
            glyphSize,
            glyphSize);

    /// <summary>Preferred size of a check box that is labelled by the row it sits in.</summary>
    public static Size MeasureGlyphOnly(int glyphSize)
        => new Size(glyphSize + (TextInset * 2), glyphSize + (TextInset * 2));

    /// <summary>Caption rectangle of a check box or radio button.</summary>
    public static Rectangle CaptionBounds(Rectangle client, int glyphSize)
        => Rectangle.FromLTRB(
            client.X + glyphSize + GlyphGap + TextInset,
            client.Y,
            client.Right,
            client.Bottom);

    /// <summary>
    /// Preferred size of a check box or radio button: the glyph box - the glyph plus
    /// its pixel of slack - and the caption with its inset on both sides. That is
    /// the composition LayoutOptions.GetPreferredSizeCore performs, so the control
    /// measures the same as a stock one.
    /// </summary>
    public static Size MeasureGlyphControl(Size caption, int glyphSize)
        => new Size(
            glyphSize + GlyphGap + caption.Width + (TextInset * 2),
            Math.Max(glyphSize, caption.Height + (TextInset * 2)));

    public static void DrawCheckBoxGlyph(Graphics graphics, Rectangle glyph, bool isChecked, PartState state)
    {
        if (!ThemeManager.Palette.IsDark && TryDrawCheckBox(graphics, glyph.Location, isChecked, state))
        {
            return;
        }

        ThemePalette palette = ThemeManager.Palette;

        if (!isChecked)
        {
            using (SolidBrush fill = new SolidBrush(GlyphFace(palette, state)))
            {
                graphics.FillRectangle(fill, glyph);
            }

            using (Pen pen = new Pen(GlyphOutline(palette, state)))
            {
                graphics.DrawRectangle(pen, glyph.X, glyph.Y, glyph.Width - 1, glyph.Height - 1);
            }

            return;
        }

        using (SolidBrush fill = new SolidBrush(
            state == PartState.Disabled ? palette.BorderDisabled : palette.Accent))
        {
            graphics.FillRectangle(fill, glyph);
        }

        // The tick is the only part that wants smoothing, and it is switched off
        // again straight away: an anti aliased one pixel frame only half covers its
        // corner pixels, which is what used to make the drop downs glow.
        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (Pen tick = new Pen(
            state == PartState.Disabled ? palette.DisabledText : palette.GlyphMark,
            Math.Max(1f, glyph.Width / 8f)))
        {
            tick.StartCap = LineCap.Round;
            tick.EndCap = LineCap.Round;
            tick.LineJoin = LineJoin.Round;
            graphics.DrawLines(tick, new[]
            {
                new PointF(glyph.Left + (glyph.Width * 0.24f), glyph.Top + (glyph.Height * 0.52f)),
                new PointF(glyph.Left + (glyph.Width * 0.44f), glyph.Top + (glyph.Height * 0.72f)),
                new PointF(glyph.Left + (glyph.Width * 0.76f), glyph.Top + (glyph.Height * 0.28f)),
            });
        }

        graphics.SmoothingMode = previous;
    }

    public static void DrawRadioGlyph(Graphics graphics, Rectangle glyph, bool isChecked, PartState state)
    {
        if (!ThemeManager.Palette.IsDark && TryDrawRadioButton(graphics, glyph.Location, isChecked, state))
        {
            return;
        }

        ThemePalette palette = ThemeManager.Palette;
        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        bool accented = isChecked && state != PartState.Disabled;
        Rectangle circle = new Rectangle(glyph.X, glyph.Y, glyph.Width - 1, glyph.Height - 1);
        using (SolidBrush fill = new SolidBrush(accented ? palette.Accent : GlyphFace(palette, state)))
        {
            graphics.FillEllipse(fill, circle);
        }

        using (Pen pen = new Pen(accented ? palette.Accent : GlyphOutline(palette, state)))
        {
            graphics.DrawEllipse(pen, circle);
        }

        if (isChecked)
        {
            // Just under a third of the glyph on each side leaves the dot Windows
            // draws: four pixels across in a thirteen pixel circle.
            int inset = Math.Max(3, (int)Math.Round(glyph.Width * 0.3f));
            Rectangle dot = Rectangle.Inflate(circle, -inset, -inset);
            using SolidBrush mark = new SolidBrush(
                state == PartState.Disabled ? palette.DisabledText : palette.GlyphMark);
            graphics.FillEllipse(mark, dot);
        }

        graphics.SmoothingMode = previous;
    }

    /// <summary>Face and frame of a push button; the caption is drawn by the caller.</summary>
    public static void DrawPushButton(Graphics graphics, Rectangle bounds, PartState state, bool isDefault)
    {
        if (!ThemeManager.Palette.IsDark && TryDrawPushButton(graphics, bounds, state, isDefault))
        {
            return;
        }

        ThemePalette palette = ThemeManager.Palette;
        using (SolidBrush face = new SolidBrush(palette.FaceFor(state)))
        {
            graphics.FillRectangle(face, bounds);
        }

        // The accent frame of a default button is the one Windows draws around the
        // button that answers Enter; hover and pressed override it.
        Color border = state == PartState.Normal && isDefault ? palette.Accent : palette.BorderFor(state);
        using (Pen pen = new Pen(border))
        {
            graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        }
    }

    /// <summary>
    /// Face, frame and chevron of a closed drop down list. The value inside it is
    /// drawn by the caller, so it is placed the same way in both themes.
    /// </summary>
    public static void DrawComboBox(Graphics graphics, Rectangle bounds, Rectangle button, PartState state)
    {
        ThemePalette palette = ThemeManager.Palette;

        if (!palette.IsDark && TryDrawPart(graphics, ComboBoxFace, state, bounds))
        {
            // CP_DROPDOWNBUTTONRIGHT is drawn over the face it shares its background
            // with, so it goes on second - and only the chevron of it is opaque.
            if (!TryDrawPart(graphics, ComboBoxChevron, state, button))
            {
                DrawChevron(graphics, button, palette.TextFor(state));
            }

            return;
        }

        // Dark: the surface follows the pointer, the way a themed field does.
        // Light without a theme (a classic desktop): a plain white field, because
        // that is what an unthemed drop down list looks like.
        using (SolidBrush face = new SolidBrush(palette.IsDark ? palette.FaceFor(state) : palette.Field))
        {
            graphics.FillRectangle(face, bounds);
        }

        using (Pen pen = new Pen(palette.BorderFor(state)))
        {
            graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        }

        DrawChevron(graphics, button, palette.TextFor(state));
    }

    /// <summary>The dotted rectangle Windows draws inside a focused control.</summary>
    public static void DrawFocusRectangle(Graphics graphics, Rectangle bounds, Color foreColor, Color backColor)
    {
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            ControlPaint.DrawFocusRectangle(graphics, bounds, foreColor, backColor);
        }
    }

    private static Color GlyphFace(ThemePalette palette, PartState state) => state switch
    {
        PartState.Hot => palette.SurfaceHot,
        PartState.Pressed => palette.SurfacePressed,
        PartState.Disabled => palette.Window,
        _ => palette.GlyphBackground,
    };

    private static Color GlyphOutline(ThemePalette palette, PartState state) => state switch
    {
        PartState.Hot or PartState.Pressed => palette.Accent,
        PartState.Disabled => palette.BorderDisabled,
        _ => palette.GlyphBorder,
    };

    /// <summary>The chevron, for the themes that do not bring one of their own.</summary>
    private static void DrawChevron(Graphics graphics, Rectangle button, Color colour)
    {
        if (button.Width <= 0 || button.Height <= 0)
        {
            return;
        }

        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        int arm = Math.Max(2, DpiScale.Round(4));
        int centreX = button.Left + (button.Width / 2);
        int centreY = button.Top + (button.Height / 2) - (arm / 2);
        using (Pen pen = new Pen(colour, Math.Max(1, DpiScale.Round(1))))
        {
            graphics.DrawLines(pen, new[]
            {
                new Point(centreX - arm, centreY),
                new Point(centreX, centreY + arm),
                new Point(centreX + arm, centreY),
            });
        }

        graphics.SmoothingMode = previous;
    }

    private static bool TryDrawCheckBox(Graphics graphics, Point location, bool isChecked, PartState state)
    {
        try
        {
            // The renderer falls back to the classic glyph on its own when the
            // desktop has no theme, so there is nothing to check first.
            CheckBoxRenderer.DrawCheckBox(graphics, location, CheckBoxStateOf(isChecked, state));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug("The themed check box glyph is unavailable: " + ex.Message);
            return false;
        }
    }

    private static bool TryDrawRadioButton(Graphics graphics, Point location, bool isChecked, PartState state)
    {
        try
        {
            RadioButtonRenderer.DrawRadioButton(graphics, location, RadioStateOf(isChecked, state));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug("The themed radio button glyph is unavailable: " + ex.Message);
            return false;
        }
    }

    private static bool TryDrawPushButton(Graphics graphics, Rectangle bounds, PartState state, bool isDefault)
    {
        try
        {
            PushButtonState push = state switch
            {
                PartState.Hot => PushButtonState.Hot,
                PartState.Pressed => PushButtonState.Pressed,
                PartState.Disabled => PushButtonState.Disabled,
                _ => isDefault ? PushButtonState.Default : PushButtonState.Normal,
            };

            ButtonRenderer.DrawButton(graphics, bounds, push);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug("The themed push button is unavailable: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Draws one uxtheme part, or reports that this theme does not define it - which
    /// is what a classic (unthemed) desktop answers for every part above.
    /// </summary>
    private static bool TryDrawPart(Graphics graphics, VisualStyleElement element, PartState state, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        try
        {
            if (!VisualStyleRenderer.IsSupported || !VisualStyleRenderer.IsElementDefined(element))
            {
                return false;
            }

            if (_renderer is null)
            {
                _renderer = new VisualStyleRenderer(element.ClassName, element.Part, (int)state);
            }
            else
            {
                _renderer.SetParameters(element.ClassName, element.Part, (int)state);
            }

            _renderer.DrawBackground(graphics, bounds);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Debug("Visual style part " + element.Part + " could not be drawn: " + ex.Message);
            return false;
        }
    }

    private static CheckBoxState CheckBoxStateOf(bool isChecked, PartState state) => state switch
    {
        PartState.Hot => isChecked ? CheckBoxState.CheckedHot : CheckBoxState.UncheckedHot,
        PartState.Pressed => isChecked ? CheckBoxState.CheckedPressed : CheckBoxState.UncheckedPressed,
        PartState.Disabled => isChecked ? CheckBoxState.CheckedDisabled : CheckBoxState.UncheckedDisabled,
        _ => isChecked ? CheckBoxState.CheckedNormal : CheckBoxState.UncheckedNormal,
    };

    private static RadioButtonState RadioStateOf(bool isChecked, PartState state) => state switch
    {
        PartState.Hot => isChecked ? RadioButtonState.CheckedHot : RadioButtonState.UncheckedHot,
        PartState.Pressed => isChecked ? RadioButtonState.CheckedPressed : RadioButtonState.UncheckedPressed,
        PartState.Disabled => isChecked ? RadioButtonState.CheckedDisabled : RadioButtonState.UncheckedDisabled,
        _ => isChecked ? RadioButtonState.CheckedNormal : RadioButtonState.UncheckedNormal,
    };
}
