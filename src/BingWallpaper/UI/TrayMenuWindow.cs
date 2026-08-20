using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// The tray menu. A borderless, top most window that behaves like a popup menu:
/// it appears at the pointer, it closes when it loses the focus or when Escape is
/// pressed, and every row is one command.
///
/// A window rather than a Win32 menu, because a Win32 menu is drawn by the system
/// and stays light on Windows 10 whatever the application asks for. This one is an
/// ordinary Avalonia window and therefore follows the theme like everything else.
///
/// The geometry is fixed rather than measured: every row declares its height, so
/// the size of the menu is known before it is on the screen, and it can be placed
/// at its final position right away instead of jumping there after the first
/// layout pass.
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    /// <summary>Width of the menu in logical pixels.</summary>
    private const double MenuWidth = 300;

    private const double SeparatorHeight = 1;

    private const double SeparatorMargin = 4;

    private const double MenuPadding = 4;

    private readonly Border _frame;
    private readonly StackPanel _rows = new() { Orientation = Orientation.Vertical };

    private double _contentHeight;

    public TrayMenuWindow()
    {
        SystemDecorations = SystemDecorations.None;
        ExtendClientAreaToDecorationsHint = false;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Width = MenuWidth;
        FontFamily = UiFonts.Default;

        _frame = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0, MenuPadding, 0, MenuPadding),
            Child = _rows,
        };
        Content = _frame;

        TitleRow = AddRow(new MenuRow("正在获取今日壁纸…", MenuRow.TitleHeight, twoLines: true));
        ToolTip.SetTip(TitleRow, "查看图片来源");
        AddSeparator();
        Older = AddRow(new MenuRow("上一张", MenuRow.CommandHeight));
        Newer = AddRow(new MenuRow("下一张", MenuRow.CommandHeight));
        History = AddRow(new MenuRow("选择日期…", MenuRow.CommandHeight));
        Refresh = AddRow(new MenuRow("立即刷新", MenuRow.CommandHeight));
        Pin = AddRow(new MenuRow("固定当前壁纸", MenuRow.CommandHeight));
        ToolTip.SetTip(Pin, "固定后不再随检查间隔自动更换");
        AddSeparator();
        Folder = AddRow(new MenuRow("打开壁纸目录", MenuRow.CommandHeight));
        AddSeparator();
        Settings = AddRow(new MenuRow("设置…", MenuRow.CommandHeight));
        Exit = AddRow(new MenuRow("退出", MenuRow.CommandHeight));

        Height = _contentHeight + (MenuPadding * 2) + (_frame.BorderThickness.Top * 2);

        // A menu that has lost the focus is a menu the user walked away from.
        Deactivated += (_, _) => Hide();

        ApplyTheme();
    }

    /// <summary>Shows the current picture and opens its copyright link.</summary>
    public MenuRow TitleRow { get; }

    public MenuRow Older { get; }

    public MenuRow Newer { get; }

    public MenuRow History { get; }

    public MenuRow Refresh { get; }

    public MenuRow Pin { get; }

    public MenuRow Folder { get; }

    public MenuRow Settings { get; }

    public MenuRow Exit { get; }

    /// <summary>Re-reads the palette for the menu and every row in it.</summary>
    public void ApplyTheme()
    {
        ThemePalette palette = ThemeManager.Palette;
        Background = palette.WindowBackground;
        _frame.Background = palette.WindowBackground;
        _frame.BorderBrush = palette.Border;

        foreach (Control child in _rows.Children)
        {
            switch (child)
            {
                case MenuRow row:
                    row.ApplyTheme();
                    break;

                case Border separator:
                    separator.Background = palette.Border;
                    break;
            }
        }
    }

    /// <summary>
    /// Puts the menu on the screen at <paramref name="anchor"/> - the pointer
    /// position the tray icon reported. It opens upwards, which is where a menu
    /// belongs with the task bar at the bottom of the screen, and flips or slides
    /// whenever that would push it off the work area.
    /// </summary>
    public void ShowAt(PixelPoint anchor)
    {
        ApplyTheme();

        Screen? screen = Screens.ScreenFromPoint(anchor) ?? Screens.Primary;
        double scaling = screen?.Scaling ?? 1.0;
        int width = (int)Math.Ceiling(Width * scaling);
        int height = (int)Math.Ceiling(Height * scaling);
        PixelRect area = screen?.WorkingArea ?? new PixelRect(anchor.X, anchor.Y, width, height);

        int x = anchor.X;
        if (x + width > area.Right)
        {
            x = anchor.X - width;
        }

        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width));

        int y = anchor.Y - height;
        if (y < area.Y)
        {
            y = anchor.Y;
        }

        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height));

        Position = new PixelPoint(x, y);

        if (!IsVisible)
        {
            Show();
        }

        Activate();

        // A window shown from a tray click does not become the foreground window on
        // its own; without this the menu would open behind whatever the user was
        // last working in, and would never receive the deactivation that closes it.
        if (TryGetPlatformHandle() is IPlatformHandle handle && handle.Handle != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(handle.Handle);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
        }
    }

    private MenuRow AddRow(MenuRow row)
    {
        _rows.Children.Add(row);
        _contentHeight += row.Height;
        return row;
    }

    private void AddSeparator()
    {
        Border separator = new Border
        {
            Height = SeparatorHeight,
            Margin = new Thickness(0, SeparatorMargin, 0, SeparatorMargin),
        };

        _rows.Children.Add(separator);
        _contentHeight += SeparatorHeight + (SeparatorMargin * 2);
    }
}
