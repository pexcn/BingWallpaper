using System;
using BingWallpaper.Theme;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace BingWallpaper.UI;

/// <summary>
/// The handful of things every window here needs and WinUI does not do on its own:
/// an icon, a fixed frame, the theme (including the title bar, which WinUI does not
/// paint), a size that follows the content and a position in the middle of the screen.
/// </summary>
internal static class WindowSupport
{
    /// <summary>The HWND behind a WinUI window - the currency of every Win32 call.</summary>
    public static IntPtr GetHandle(Window window) => WindowNative.GetWindowHandle(window);

    /// <summary>
    /// Gives a window its title, its icon and, unless it asks otherwise, a frame that
    /// cannot be resized or maximised: a dialog is sized to exactly its content, so
    /// there is nothing for a resize to do except add empty space.
    /// </summary>
    public static void Prepare(Window window, string title, bool resizable = false)
    {
        window.Title = title;

        IntPtr handle = GetHandle(window);
        uint dpi = GetDpi(handle);

        // The window keeps using this HICON for as long as it exists, so it is not
        // destroyed here: these windows live as long as the process does.
        IntPtr icon = AppIcon.LoadWindowIcon(dpi);
        if (icon != IntPtr.Zero)
        {
            try
            {
                window.AppWindow.SetIcon(Win32Interop.GetIconIdFromIcon(icon));
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not set the window icon: " + ex.Message);
            }
        }

        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = resizable;
            presenter.IsMaximizable = resizable;
            presenter.IsMinimizable = false;
        }
    }

    /// <summary>
    /// Repaints the window in the current theme. The content follows
    /// <see cref="ElementTheme"/>; the caption is drawn by the desktop window manager
    /// and needs the DWM attribute instead.
    /// </summary>
    public static void ApplyTheme(Window window)
    {
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = ThemeManager.ElementTheme;
        }

        DarkModeNative.ApplyTitleBar(GetHandle(window), ThemeManager.IsDark);
    }

    /// <summary>
    /// Sizes the window to what its content asked for. The measurement is in
    /// effective (layout) pixels, the window is in physical ones, so the result is
    /// scaled by the rasterization scale of this window's monitor.
    /// </summary>
    public static void ResizeToContent(Window window, FrameworkElement root, double minWidth, double minHeight)
    {
        try
        {
            root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double scale = root.XamlRoot is not null ? root.XamlRoot.RasterizationScale : 1.0;
            if (scale <= 0)
            {
                scale = 1.0;
            }

            int width = (int)Math.Ceiling(Math.Max(root.DesiredSize.Width, minWidth) * scale);
            int height = (int)Math.Ceiling(Math.Max(root.DesiredSize.Height, minHeight) * scale);
            window.AppWindow.ResizeClient(new SizeInt32(width, height));
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not size the window to its content: " + ex.Message);
        }
    }

    /// <summary>
    /// Sizes the client area from logical (96 DPI) pixels. Used where the content
    /// cannot measure itself into a sensible window - a grid of thumbnails is as
    /// large as it is allowed to be, not as large as it wants to be.
    /// </summary>
    public static void ResizeLogical(Window window, double width, double height)
    {
        try
        {
            double scale = GetDpi(GetHandle(window)) / 96.0;
            window.AppWindow.ResizeClient(new SizeInt32(
                (int)Math.Ceiling(width * scale),
                (int)Math.Ceiling(height * scale)));
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not size the window: " + ex.Message);
        }
    }

    /// <summary>Centres the window on the work area of the monitor it is on.</summary>
    public static void Center(Window window)
    {
        try
        {
            AppWindow appWindow = window.AppWindow;
            DisplayArea area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
            RectInt32 work = area.WorkArea;
            SizeInt32 size = appWindow.Size;
            appWindow.Move(new PointInt32(
                work.X + Math.Max(0, (work.Width - size.Width) / 2),
                work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not centre the window: " + ex.Message);
        }
    }

    /// <summary>Brings a window up whether it was hidden, minimised or already open.</summary>
    public static void ShowAndActivate(Window window)
    {
        AppWindow appWindow = window.AppWindow;
        if (!appWindow.IsVisible)
        {
            appWindow.Show();
        }

        if (appWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        window.Activate();

        // Activate() alone does not always win the foreground race against whatever
        // the user was doing when they clicked the tray icon.
        NativeMethods.SetForegroundWindow(GetHandle(window));
    }

    private static uint GetDpi(IntPtr handle)
    {
        try
        {
            uint dpi = handle != IntPtr.Zero ? NativeMethods.GetDpiForWindow(handle) : 0;
            return dpi == 0 ? NativeMethods.GetSystemDpiSafe() : dpi;
        }
        catch (Exception ex)
        {
            Logger.Debug("GetDpiForWindow failed: " + ex.Message);
            return 96u;
        }
    }
}
